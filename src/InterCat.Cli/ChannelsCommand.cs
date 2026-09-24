using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Read-only pages of paired TCP channels, including generations above the overview bound.</summary>
internal static class ChannelsCommand
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
        string? processText = command.TakeOption("--process");
        string? cursor = command.TakeOption("--cursor");
        string? size = command.TakeOption("--page-size");
        int pageSize = SessionChannelQuery.DefaultPageSize;
        bool invalidSize = size is not null &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SessionChannelQuery.MaximumPageSize);
        bool invalidProcess = processText is not null
            && (!Guid.TryParse(processText, out Guid parsed) || parsed == Guid.Empty);
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown || invalidProcess || invalidSize)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat channels <directory>."
                : hasUnknown ? $"Unknown or incomplete option: {unknown}"
                : invalidProcess
                    ? "--process must be a process-instance GUID from the overview."
                    : "--page-size must be an integer from 1 to 200.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessInstanceId? scope = processText is null ? null : new(Guid.Parse(processText));
        SessionChannelPage page;
        try
        {
            page = SessionChannelQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                scope, pageSize: pageSize, cursor: cursor,
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
                Contract = "channel-page-v1", SessionPath = path, Page = page,
            }, JsonContracts.Indented));
            return page.RestartRequired ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Admitted paired TCP channels");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(page.Generation));
        if (scope is not null) ConsoleUi.Field("Process instance", scope.ToString()!);
        if (page.RestartRequired)
        {
            ConsoleUi.Warn(page.RestartReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        ConsoleUi.Field("Channels in scope", ConsoleUi.Count(page.TotalChannels));
        foreach (Channel channel in page.Channels)
            ConsoleUi.Line($"  {channel.Key} · {channel.Name} · {channel.ObservationCount:N0} observed records");
        ConsoleUi.Note(page.Caveat);
        if (page.NextCursor is not null)
            ConsoleUi.Note($"Next page: icat channels <directory> --cursor {page.NextCursor}"
                + (scope is null ? string.Empty : $" --process {scope}"));
        ConsoleUi.Note("Use icat evidence <directory> --channel <key> for this channel's exact rows.");
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat channels <session-directory> [--process <instance-guid>] [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted paired TCP channels, even above the overview bound.");
        ConsoleUi.Line("  A stale cursor requests an explicit restart; --process scopes by stable instance ID.");
    }
}
