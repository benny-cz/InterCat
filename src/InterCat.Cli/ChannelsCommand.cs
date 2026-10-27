using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Read-only pages of paired TCP and UDP channels, including generations above the overview bound.</summary>
internal static class ChannelsCommand
{
    /// <summary>
    /// The JSON's contract: a page of channels and the cursor to the next, since its second the busiest first by the
    /// records at their two ends, channels of as many records by key, where its first paged them in key order alone.
    /// </summary>
    internal const string Contract = "channel-page-v2";

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
        bool json = command.TryTakeFlag("--json");
        bool oneSided = command.TryTakeFlag("--one-sided");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown || invalidSize)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat channels <directory>."
                : hasUnknown ? command.Refusal(unknown!)
                : string.Create(CultureInfo.CurrentCulture,
                        $"--page-size expects a number of channels from 1 to {SessionChannelQuery.MaximumPageSize:N0}; '{size}' is not one."));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (oneSided && processText is null)
        {
            return Refuse("--one-sided lists one process's connections: name it with --process <pid | instance>.");
        }

        // A process is named by a PID one process alone held, else by its instance, as every command names one (P6); one
        // naming no process of the session is refused in words, never listed as a process with no channel.
        ProcessInstanceId? scope = null;
        if (processText is not null)
        {
            ConsoleUi.Progress("Projecting the current generation, to find the process --process names.");
            Dictionary<ProcessInstanceId, ProcessNode> processes;
            try
            {
                processes = SessionOverviewProjector.Project(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                    followRpcCalls: false, cancellationToken: cancellationToken).Nodes.ToDictionary(node => node.Id);
            }
            catch (InvalidOperationException exception) when (exception is not NoSessionException)
            {
                ConsoleUi.Failure(exception.Message);
                return InterCatExitCode.PermissionOrCapabilityFailure;
            }

            if (ProcessOption.Resolve(processes, "--process", processText, out string? unresolved) is not { } named)
            {
                ConsoleUi.Failure(unresolved!);
                return InterCatExitCode.InvalidInvocation;
            }

            scope = named;
        }

        if (oneSided)
        {
            return OneSided(path, scope!.Value, json, cancellationToken);
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
                Contract = Contract, SessionPath = path, Page = page,
            }, JsonContracts.Indented));
            return page.RestartRequired ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Admitted paired TCP and UDP channels");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(page.Generation));
        if (scope is not null) ConsoleUi.Field("Process instance", scope.ToString()!);
        if (page.RestartRequired)
        {
            ConsoleUi.Warn(page.RestartReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        ConsoleUi.Field("Channels in scope", ConsoleUi.Count(page.TotalChannels));
        ConsoleUi.Field("Ranked by", "the records at their two ends, the busiest first");
        // Each channel by who is at its two ends and who connected to whom, as the window's list names it, then the key its
        // records are read by.
        foreach (Channel channel in page.Channels)
        {
            ConsoleUi.Line("  " + ChannelNames.Ends(channel, id => page.ProcessNames.GetValueOrDefault(id.ToString()))
                + " · " + CountText.Of(channel.ObservationCount, "observed record"));
            ConsoleUi.Line("    " + channel.Key);

            // How it was paired, in the inspector's words (R18): the rule and how strongly, its lifetime, and the coverage.
            if (channel.Rule is not null)
            {
                ConsoleUi.Line("    " + ChannelPairingText.Explain(channel));
            }
        }
        // Whether a channel listed nowhere could have been seen at all is the ledger's to say (R21), of each transport.
        foreach (MechanismCoverage coverage in page.Coverage)
        {
            ConsoleUi.Note(SessionCoverage.Sentence(coverage, "the session"));
        }

        ConsoleUi.Note(page.Caveat);
        if (page.NextCursor is not null)
            ConsoleUi.Note($"Next page: icat channels <directory> --cursor {page.NextCursor}"
                + (scope is null ? string.Empty : $" --process {scope}"));
        ConsoleUi.Note("Use icat evidence <directory> --channel <key> for a channel's exact rows, by the key beneath its name.");
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
                // Since its second version each connection says how the capture saw it open: connected, accepted or neither.
                Contract = "connection-list-v2", SessionPath = path, ProcessInstance = instance.ToString(), List = list,
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
                $"  {connection.Name} {connection.OwnEnd} · {CountText.Of(connection.Records, "record")} · {connection.Transfers(CultureInfo.CurrentCulture)} · {connection.Lifetime}"));
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
        ConsoleUi.Line("icat channels <session-directory> [--process <pid | instance> [--one-sided]] [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted paired TCP and UDP channels, even above the overview bound, the");
        ConsoleUi.Line("  busiest first by the records at their two ends, each named by the processes at its two ends - the");
        ConsoleUi.Line("  one that made it first, an arrow at the one it connected to - with its key, and what the capture");
        ConsoleUi.Line("  covered of each transport, from its coverage ledger, so a page of none is not read as no traffic.");
        ConsoleUi.Line("  A stale cursor requests an explicit restart; --process scopes to one process, by the PID it alone");
        ConsoleUi.Line("  held or its instance id as icat processes prints it.");
        ConsoleUi.Line("  --one-sided lists the process's connections whose other end no record holds - most often");
        ConsoleUi.Line("  another host's - with their records and bytes, as its rung does.");
    }
}
