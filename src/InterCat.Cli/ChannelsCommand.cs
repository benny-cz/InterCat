using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
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

        string? processText = command.TakeOption("--process");
        string? cursor = command.TakeOption("--cursor");
        string? size = command.TakeOption("--page-size");
        string? directory = command.TakePositional();
        int pageSize = SessionChannelQuery.DefaultPageSize;
        bool invalidSize = size is not null &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SessionChannelQuery.MaximumPageSize);
        bool invalidProcess = processText is not null
            && (!Guid.TryParse(processText, out Guid parsed) || parsed == Guid.Empty);
        bool json = command.TryTakeFlag("--json");
        bool oneSided = command.TryTakeFlag("--one-sided");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown || invalidProcess || invalidSize)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat channels <directory>."
                : hasUnknown ? CommandLine.Unknown(unknown!)
                : invalidProcess
                    ? "--process must be a process-instance GUID from the overview."
                    : "--page-size must be an integer from 1 to 200.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessInstanceId? scope = processText is null ? null : new(Guid.Parse(processText));
        if (oneSided)
        {
            return scope is { } instance
                ? OneSided(path, instance, json, cancellationToken)
                : Refuse("--one-sided lists one process's connections: name it with --process <instance-guid>.");
        }

        SessionChannelPage page;
        try
        {
            page = SessionChannelQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                scope, pageSize: pageSize, cursor: cursor,
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
        {
            ConsoleUi.Line($"  {channel.Key} · {channel.Name} · {CountText.Of(channel.ObservationCount, "observed record")}");

            // How it was paired, in the inspector's words (R18): the rule and how strongly, its lifetime, and the coverage.
            if (channel.Rule is not null)
            {
                ConsoleUi.Line("    " + ChannelPairingText.Explain(channel));
            }
        }
        // Whether a channel listed nowhere could have been seen at all is the ledger's to say (R21).
        ConsoleUi.Note(SessionCoverage.Sentence(page.Coverage[0], "the session"));
        ConsoleUi.Note(page.Caveat);
        if (page.NextCursor is not null)
            ConsoleUi.Note($"Next page: icat channels <directory> --cursor {page.NextCursor}"
                + (scope is null ? string.Empty : $" --process {scope}"));
        ConsoleUi.Note("Use icat evidence <directory> --channel <key> for this channel's exact rows.");
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// One process's one-sided connections (§7.1): the TCP connections and UDP flows it held whose other end no record
    /// holds, as its rung lists them, each with the key icat evidence --channel reads its records by.
    /// </summary>
    private static InterCatExitCode OneSided(string path, ProcessInstanceId instance, bool json, CancellationToken cancellationToken)
    {
        ConnectionList list;
        try
        {
            list = SessionConnections.OneSided(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), instance,
                cancellationToken: cancellationToken);
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
                Contract = "connection-list-v1", SessionPath = path, ProcessInstance = instance.ToString(), List = list,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Connections no record's other end holds");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(list.Generation));
        ConsoleUi.Field("Process instance", instance.ToString());
        ConsoleUi.Field("Connections", ConsoleUi.Count(list.Connections.Count));
        foreach (ConnectionSummary connection in list.Connections)
        {
            ConsoleUi.Line(string.Create(CultureInfo.CurrentCulture,
                $"  {connection.Name} from {connection.LocalEndpoint} · {CountText.Of(connection.Records, "record")} · {connection.Transfers(CultureInfo.CurrentCulture)} · {connection.Lifetime}"));
            ConsoleUi.Line("    " + connection.Key);
        }

        ConsoleUi.Note("No record of this capture holds these connections' other ends, so nothing is said of who is there: "
            + "most are other hosts'. Bytes are what this process's own transfer records measured.");
        ConsoleUi.Note("Use icat evidence <directory> --channel <key> for a connection's exact rows.");
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Refuse(string reason)
    {
        ConsoleUi.Failure(reason);
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat channels <session-directory> [--process <instance-guid> [--one-sided]] [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted paired TCP channels, even above the overview bound, with what the");
        ConsoleUi.Line("  capture covered of TCP, from its coverage ledger, so a page of none is not read as no traffic.");
        ConsoleUi.Line("  A stale cursor requests an explicit restart; --process scopes by stable instance ID.");
        ConsoleUi.Line("  --one-sided lists the process's connections whose other end no record holds - most often");
        ConsoleUi.Line("  another host's - with their records and bytes, as its rung does.");
    }
}
