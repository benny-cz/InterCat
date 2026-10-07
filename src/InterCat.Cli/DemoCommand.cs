using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>What `icat demo --json` answers: where the demo was written, and what it is.</summary>
internal sealed record DemoDocument
{
    public required string Contract { get; init; }
    public required string Directory { get; init; }
    public required string Investigation { get; init; }
    public required IReadOnlyList<string> Sessions { get; init; }
    public required string SourceIdentity { get; init; }
    public required string Disclosure { get; init; }
}

/// <summary>
/// `icat demo`: writes InterCat's generated demo investigation (<see cref="DemoInvestigation"/>) into a new folder, so a
/// person can try every view of an investigation of two hosts without capturing anything. It says what it is first.
/// </summary>
internal static class DemoCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return Task.FromResult(InterCatExitCode.Success);
        }

        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        if (directory is null)
        {
            ConsoleUi.Failure("Say where the demo goes: a new or empty folder.");
            ConsoleUi.Explain(PrintHelp);
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        string full = Path.GetFullPath(directory);
        if (File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()))
        {
            ConsoleUi.Failure($"{full} already holds something; the demo is written only into a new or empty folder.");
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        ConsoleUi.Progress(DemoInvestigation.Disclosure + " Writing its two sessions and the investigation that holds them.");
        DemoInvestigationResult made = DemoInvestigation.Create(full, cancellationToken);
        var document = new DemoDocument
        {
            Contract = DemoInvestigation.SourceIdentity,
            Directory = made.Directory,
            Investigation = made.WorkspacePath,
            Sessions = [made.ClientSession, made.ServerSession],
            SourceIdentity = DemoInvestigation.SourceIdentity,
            Disclosure = DemoInvestigation.Disclosure,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            ConsoleUi.Heading("Demo investigation");
            ConsoleUi.Field("Investigation", made.WorkspacePath);
            ConsoleUi.Field("Demo client", made.ClientSession);
            ConsoleUi.Field("Demo server", made.ServerSession);
            ConsoleUi.Note(DemoInvestigation.Disclosure);
            ConsoleUi.Note($"Open it with: icat workspace show \"{made.WorkspacePath}\" (or correlate, timeline), or in the "
                + "window's Investigation menu.");
        }

        return Task.FromResult(InterCatExitCode.Success);
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat demo <directory> [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Writes InterCat's generated demo into a new or empty folder: an investigation of two hosts that");
        ConsoleUi.Line("  exchange TCP, one of which also sends UDP to a host neither recorded, each with a session of its");
        ConsoleUi.Line("  own, aligned by their wall clocks. Nothing in it was captured, and every view of it says so.");
        ConsoleUi.Line("  Its sessions are the same, record for record, every time it is written.");
    }
}
