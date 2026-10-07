using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>A support bundle as `icat support` writes it (`contracts/support-bundle-v1.md`).</summary>
internal sealed record SupportBundleDocument
{
    public required string Contract { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string Version { get; init; }
    public required SupportRuntime Runtime { get; init; }

    /// <summary>This machine's capability report (`icat capabilities`), which names no machine, host or user.</summary>
    public required CapabilityReport Capabilities { get; init; }

    public required IReadOnlyList<SupportSession> Sessions { get; init; }

    /// <summary>What the bundle holds and what it leaves out, as its listing said before it was written.</summary>
    public required IReadOnlyList<string> Holds { get; init; }

    public required IReadOnlyList<string> LeftOut { get; init; }
}

/// <summary>
/// `icat support`: §20.6's support bundle. It lists what it holds and what it leaves out before it reads or writes
/// anything, and holds nothing a record holds - no payload, endpoint, command line, raw record or name (P16).
/// </summary>
internal static class SupportCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? output = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool check = command.TryTakeFlag("--check");
        bool json = command.TryTakeFlag("--json");
        var sessions = new List<string>();
        while (command.TakePositional() is { } session)
        {
            sessions.Add(session);
        }

        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (check && (output is not null || json))
        {
            ConsoleUi.Failure("--check lists what a bundle would hold and writes nothing, so it takes neither --output nor --json.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!check && output is null && !json)
        {
            ConsoleUi.Failure("Say where the bundle goes: --output <path> writes it, --json prints it, and --check lists what "
                + "it would hold.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string? target = output is null ? null : Path.GetFullPath(output);
        if (target is not null && File.Exists(target) && !overwrite)
        {
            ConsoleUi.Failure($"{target} already exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessions.FirstOrDefault(session => !Directory.Exists(session)) is { } missing)
        {
            ConsoleUi.Failure($"No session directory at {Path.GetFullPath(missing)}.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Its contents are listed before anything is read or written (§20.6); with --json, where the bundle is the output,
        // the listing goes beside it.
        IReadOnlyList<string> holds = SupportBundle.Holds(sessions.Count, capabilities: true);
        Action<string> say = json ? ConsoleUi.Progress : ConsoleUi.Note;
        if (!json)
        {
            ConsoleUi.Heading("Support bundle");
        }

        say("Holds: " + string.Join("; ", holds) + ".");
        say("Leaves out: " + string.Join("; ", SupportBundle.LeftOut) + ".");
        if (check)
        {
            return InterCatExitCode.Success;
        }

        ConsoleUi.Progress("Reading this machine's capability report and each session. No capture is started.");
        var document = new SupportBundleDocument
        {
            Contract = SupportBundle.Contract,
            CreatedUtc = DateTimeOffset.UtcNow,
            Version = SupportBundle.ProductVersion,
            Runtime = SupportBundle.Runtime(),
            Capabilities = CapabilitiesCommand.Probe(),
            Sessions = [.. sessions.Select(session => SupportBundle.DescribeSession(session, cancellationToken))],
            Holds = holds,
            LeftOut = SupportBundle.LeftOut,
        };
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            foreach (SupportSession session in document.Sessions)
            {
                ConsoleUi.Field(session.Folder, session.Problem is { } problem
                    ? "could not be read in full: " + problem
                    : string.Create(CultureInfo.CurrentCulture, $"generation {session.Generation:N0}, ")
                        + CountText.Of(session.Rows, "row") + ", " + CountText.Of(session.Dependencies.Count, "file"));
            }
        }

        if (target is not null)
        {
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(target, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Support bundle written to {target}");
        }

        return document.Sessions.Any(session => session.Problem is not null)
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat support [<session> ...] (--output <path> [--overwrite] | --json | --check)");
        ConsoleUi.Line();
        ConsoleUi.Line("  A support bundle: InterCat's version, this machine's runtime and capability report, and for each");
        ConsoleUi.Line("  session named its folder's name, generation and files, rows by mechanism and coverage, coverage");
        ConsoleUi.Line("  ledger and loss counters, clock and recording. It holds no message content, endpoint, command line,");
        ConsoleUi.Line("  raw record, name or path, and lists what it holds and leaves out before it reads anything.");
        ConsoleUi.Line("  --check lists them and writes nothing. A session that cannot be read is said to be, with why.");
    }
}
